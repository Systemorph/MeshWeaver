namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Persistence service for MeshNode instances in a hierarchical graph structure.
/// The graph root is at address "_graph" (path "/").
/// Each path segment manages its children (segment1 manages segment1/*, etc.)
/// </summary>
public interface IPersistenceService
{
    /// <summary>
    /// Gets a node by its path.
    /// </summary>
    /// <param name="path">The node path (e.g., "org/acme/project/web")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The node or null if not found</returns>
    Task<MeshNode?> GetNodeAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Gets all child nodes at the specified parent path.
    /// </summary>
    /// <param name="parentPath">Parent path (empty or null for root level)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Collection of child nodes</returns>
    Task<IEnumerable<MeshNode>> GetChildrenAsync(string? parentPath, CancellationToken ct = default);

    /// <summary>
    /// Gets all descendant nodes under the specified path.
    /// </summary>
    /// <param name="parentPath">Parent path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Collection of all descendant nodes</returns>
    Task<IEnumerable<MeshNode>> GetDescendantsAsync(string? parentPath, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a node.
    /// </summary>
    /// <param name="node">The node to save</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The saved node</returns>
    Task<MeshNode> SaveNodeAsync(MeshNode node, CancellationToken ct = default);

    /// <summary>
    /// Deletes a node and optionally its descendants.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="recursive">If true, also delete all descendants</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default);

    /// <summary>
    /// Searches nodes by query text within their Name, Description, or Content.
    /// </summary>
    /// <param name="parentPath">Parent path to search under (null for all)</param>
    /// <param name="query">Search query</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Matching nodes</returns>
    Task<IEnumerable<MeshNode>> SearchAsync(string? parentPath, string query, CancellationToken ct = default);

    /// <summary>
    /// Checks if a node exists at the given path.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the node exists</returns>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Initializes the persistence service.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task InitializeAsync(CancellationToken ct = default);
}
