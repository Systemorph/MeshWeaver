using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service for loading and saving graph configuration from persistence.
/// All configuration is stored in _config/ partition.
/// </summary>
public interface IConfigurationStorageService
{
    /// <summary>
    /// The base data directory for content collections and persistence.
    /// </summary>
    string DataDirectory { get; }
    /// <summary>
    /// Loads all configuration objects from _config/ partition.
    /// Returns DataModel, LayoutAreaConfig, ContentCollectionConfig, HubFeatureConfig, NodeTypeConfig objects.
    /// </summary>
    IAsyncEnumerable<object> LoadAllAsync(MeshNode node, CancellationToken ct = default);

    /// <summary>
    /// Saves a configuration object to _config/{type}/{id}.json.
    /// </summary>
    Task SaveAsync(object config, CancellationToken ct = default);

    /// <summary>
    /// Deletes a configuration item by type and ID.
    /// </summary>
    Task DeleteAsync<T>(string id, CancellationToken ct = default);

    /// <summary>
    /// Loads a specific configuration item by type and ID.
    /// </summary>
    /// <typeparam name="T">The configuration type (DataModel, LayoutAreaConfig, etc.)</typeparam>
    /// <param name="id">The configuration ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The configuration item or null if not found</returns>
    Task<T?> LoadByIdAsync<T>(string id, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Loads all configuration items of a specific type.
    /// </summary>
    /// <typeparam name="T">The configuration type (DataModel, LayoutAreaConfig, etc.)</typeparam>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all items of the specified type</returns>
    Task<IReadOnlyList<T>> LoadAllAsync<T>(CancellationToken ct = default) where T : class;
}
