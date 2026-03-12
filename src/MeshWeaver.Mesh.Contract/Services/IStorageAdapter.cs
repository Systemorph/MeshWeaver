using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Low-level storage adapter for persistence implementations.
/// Abstracts the actual storage mechanism (file system, Cosmos DB, etc.)
/// </summary>
public interface IStorageAdapter
{
    /// <summary>
    /// Reads a node from storage.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The node or null if not found</returns>
    Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default);

    /// <summary>
    /// Writes a node to storage.
    /// </summary>
    /// <param name="node">The node to write</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default);

    /// <summary>
    /// Deletes a node from storage.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Lists child paths under a parent path.
    /// Returns both node paths (JSON files) and directory paths (folders without nodes but with content).
    /// </summary>
    /// <param name="parentPath">Parent path (empty for root level)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Tuple of (nodePaths, directoryPaths) - nodePaths are paths with JSON files, directoryPaths are folders to scan recursively</returns>
    Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(string? parentPath, CancellationToken ct = default);

    /// <summary>
    /// Checks if a node exists at the given path.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the node exists</returns>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Finds the node whose path is the longest prefix of the given full path.
    /// For example, given "Organization/acme/Settings", finds "Organization/acme" if it exists.
    /// Default implementation returns null (not supported — caller falls back to iterative lookup).
    /// </summary>
    /// <param name="fullPath">The full path to find the best prefix match for</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The matching node and number of matched segments, or (null, 0) if not found</returns>
    Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        string fullPath, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.FromResult<(MeshNode?, int)>((null, 0));

    /// <summary>
    /// Lists partition sub-paths for a node (subdirectories that contain partition data, not child nodes).
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Sub-path names (e.g., "_Source", "layoutAreas")</returns>
    Task<IEnumerable<string>> ListPartitionSubPathsAsync(string nodePath, CancellationToken ct = default)
        => Task.FromResult<IEnumerable<string>>(Enumerable.Empty<string>());

    #region Partition Storage

    /// <summary>
    /// Loads all objects from a node's partition folder.
    /// Objects are deserialized using $type discriminators for polymorphic serialization.
    /// </summary>
    /// <param name="nodePath">The node path (e.g., "_types/story")</param>
    /// <param name="subPath">Optional sub-path within partition (e.g., "layoutAreas")</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async enumerable of deserialized objects</returns>
    IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath, JsonSerializerOptions options, CancellationToken ct = default);

    /// <summary>
    /// Saves objects to a node's partition folder.
    /// Each object is stored as a separate JSON file with $type discriminator.
    /// File names are derived from the object's Id property if available.
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="subPath">Optional sub-path within partition</param>
    /// <param name="objects">Objects to save</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default);

    /// <summary>
    /// Deletes all objects from a node's partition folder (or sub-path).
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="subPath">Optional sub-path within partition</param>
    /// <param name="ct">Cancellation token</param>
    Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default);

    /// <summary>
    /// Gets the newest modification timestamp across all objects in a partition (or sub-path).
    /// Used for cache invalidation.
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="subPath">Optional sub-path within partition</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The newest modification timestamp, or null if no objects exist</returns>
    Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default);

    #endregion
}
