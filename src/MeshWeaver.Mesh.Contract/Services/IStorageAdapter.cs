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
    /// <param name="ct">Cancellation token</param>
    /// <returns>The node or null if not found</returns>
    Task<MeshNode?> ReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Writes a node to storage.
    /// </summary>
    /// <param name="node">The node to write</param>
    /// <param name="ct">Cancellation token</param>
    Task WriteAsync(MeshNode node, CancellationToken ct = default);

    /// <summary>
    /// Deletes a node from storage.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Lists child paths under a parent path.
    /// </summary>
    /// <param name="parentPath">Parent path (empty for root level)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Collection of child paths</returns>
    Task<IEnumerable<string>> ListChildPathsAsync(string? parentPath, CancellationToken ct = default);

    /// <summary>
    /// Checks if a node exists at the given path.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the node exists</returns>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
}
