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
    /// <returns>Async enumerable of child nodes</returns>
    IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath);

    /// <summary>
    /// Gets all descendant nodes under the specified path.
    /// </summary>
    /// <param name="parentPath">Parent path</param>
    /// <returns>Async enumerable of all descendant nodes</returns>
    IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath);

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
    /// Moves a node and all its descendants to a new path.
    /// Comments associated with moved nodes are also migrated.
    /// </summary>
    /// <param name="sourcePath">The current node path</param>
    /// <param name="targetPath">The new node path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The moved node at the new path</returns>
    /// <exception cref="InvalidOperationException">If source doesn't exist or target already exists</exception>
    Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, CancellationToken ct = default);

    /// <summary>
    /// Searches nodes by query text within their Name, Description, or Content.
    /// </summary>
    /// <param name="parentPath">Parent path to search under (null for all)</param>
    /// <param name="query">Search query</param>
    /// <returns>Async enumerable of matching nodes</returns>
    IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query);

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

    #region Comments

    /// <summary>
    /// Gets all comments for a node.
    /// </summary>
    /// <param name="nodePath">Path of the node</param>
    /// <returns>Async enumerable of comments for the node</returns>
    IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath);

    /// <summary>
    /// Adds a comment to a node.
    /// </summary>
    /// <param name="comment">The comment to add</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The saved comment</returns>
    Task<Comment> AddCommentAsync(Comment comment, CancellationToken ct = default);

    /// <summary>
    /// Deletes a comment by ID.
    /// </summary>
    /// <param name="commentId">The comment ID to delete</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteCommentAsync(string commentId, CancellationToken ct = default);

    /// <summary>
    /// Gets a single comment by ID.
    /// </summary>
    /// <param name="commentId">The comment ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The comment or null if not found</returns>
    Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default);

    #endregion

    #region Partition Storage

    /// <summary>
    /// Gets all objects from a node's partition folder.
    /// Objects are stored as separate JSON files with $type discriminators.
    /// </summary>
    /// <param name="nodePath">The node path (e.g., "_types/story")</param>
    /// <param name="subPath">Optional sub-path within partition (e.g., "layoutAreas")</param>
    /// <returns>Async enumerable of deserialized objects</returns>
    IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath = null);

    /// <summary>
    /// Saves objects to a node's partition folder.
    /// Each object is stored as a separate JSON file with $type discriminator.
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="subPath">Optional sub-path within partition</param>
    /// <param name="objects">Objects to save</param>
    /// <param name="ct">Cancellation token</param>
    Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, CancellationToken ct = default);

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

    #region Secure Operations

    /// <summary>
    /// Gets a node by path, applying security filtering.
    /// Returns null if the user doesn't have read permission.
    /// Default implementation delegates to GetNodeAsync (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The node or null if not found or not authorized</returns>
    Task<MeshNode?> GetNodeSecureAsync(string path, string? userId, CancellationToken ct = default)
        => GetNodeAsync(path, ct);

    /// <summary>
    /// Gets child nodes, filtering out those the user cannot read.
    /// Default implementation delegates to GetChildrenAsync (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="parentPath">Parent path (empty or null for root level)</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <returns>Async enumerable of accessible child nodes</returns>
    IAsyncEnumerable<MeshNode> GetChildrenSecureAsync(string? parentPath, string? userId)
        => GetChildrenAsync(parentPath);

    /// <summary>
    /// Gets descendant nodes, filtering out those the user cannot read.
    /// Default implementation delegates to GetDescendantsAsync (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="parentPath">Parent path</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <returns>Async enumerable of accessible descendant nodes</returns>
    IAsyncEnumerable<MeshNode> GetDescendantsSecureAsync(string? parentPath, string? userId)
        => GetDescendantsAsync(parentPath);

    #endregion
}
