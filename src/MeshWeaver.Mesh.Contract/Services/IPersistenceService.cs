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
}
