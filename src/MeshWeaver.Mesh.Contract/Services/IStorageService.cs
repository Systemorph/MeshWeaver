using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Reactive;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Core persistence service for MeshNode instances in a hierarchical graph structure.
/// The graph root is at address "_graph" (path "/").
/// Each path segment manages its children (segment1 manages segment1/*, etc.)
/// This is the internal interface that accepts JsonSerializerOptions per method.
/// Use IMeshStorage for the scoped wrapper that injects options automatically.
/// </summary>
internal interface IStorageService
{
    /// <summary>
    /// Gets a node by its path. Returns an observable that emits the node (or null
    /// if not found) and completes. The Task → IObservable bridge lives inside the
    /// implementation so callers compose with <c>SelectMany</c>/<c>Subscribe</c>
    /// instead of bridging at every call site.
    /// </summary>
    /// <param name="path">The node path (e.g., "org/acme/project/web")</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Observable emitting the node or null</returns>
    IObservable<MeshNode?> GetNode(string path, JsonSerializerOptions options);

    /// <summary>
    /// Gets all child nodes at the specified parent path.
    /// </summary>
    /// <param name="parentPath">Parent path (empty or null for root level)</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Async enumerable of child nodes</returns>
    IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath, JsonSerializerOptions options);

    /// <summary>
    /// Gets ALL child nodes including satellites (MainNode != Path).
    /// Used by filtered queries (e.g., nodeType:Thread) that need to find satellite children.
    /// Default implementation delegates to GetChildrenAsync (excludes satellites).
    /// </summary>
    IAsyncEnumerable<MeshNode> GetAllChildrenAsync(string? parentPath, JsonSerializerOptions options)
        => GetChildrenAsync(parentPath, options);

    /// <summary>
    /// Gets all descendant nodes under the specified path.
    /// </summary>
    /// <param name="parentPath">Parent path</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Async enumerable of all descendant nodes</returns>
    IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath, JsonSerializerOptions options);

    /// <summary>
    /// Gets ALL descendant nodes including satellites (MainNode != Path).
    /// Used by filtered queries (e.g., nodeType:Thread) that need to find satellite nodes.
    /// Default implementation delegates to GetDescendantsAsync (excludes satellites).
    /// </summary>
    IAsyncEnumerable<MeshNode> GetAllDescendantsAsync(string? parentPath, JsonSerializerOptions options)
        => GetDescendantsAsync(parentPath, options);

    /// <summary>
    /// Creates or updates a node.
    /// </summary>
    /// <param name="node">The node to save</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The saved node</returns>
    Task<MeshNode> SaveNodeAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default);

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
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The moved node at the new path</returns>
    /// <exception cref="InvalidOperationException">If source doesn't exist or target already exists</exception>
    Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, JsonSerializerOptions options, CancellationToken ct = default);

    /// <summary>
    /// Searches nodes by query text within their Name or Content.
    /// </summary>
    /// <param name="parentPath">Parent path to search under (null for all)</param>
    /// <param name="query">Search query</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Async enumerable of matching nodes</returns>
    IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query, JsonSerializerOptions options);

    /// <summary>
    /// Checks if a node exists at the given path.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the node exists</returns>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Finds the node whose path is the longest prefix of the given full path.
    /// Uses a single query instead of iterating through ancestor paths.
    /// </summary>
    /// <param name="fullPath">The full path to find the best prefix match for</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The matching node and number of matched segments, or (null, 0) if not found</returns>
    Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        string fullPath, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.FromResult<(MeshNode?, int)>((null, 0));

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
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Async enumerable of comments for the node</returns>
    IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath, JsonSerializerOptions options);

    /// <summary>
    /// Adds a comment to a node.
    /// </summary>
    /// <param name="comment">The comment to add</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The saved comment</returns>
    Task<Comment> AddCommentAsync(Comment comment, JsonSerializerOptions options, CancellationToken ct = default);

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
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Async enumerable of deserialized objects</returns>
    IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath, JsonSerializerOptions options);

    /// <summary>
    /// Saves objects to a node's partition folder.
    /// Each object is stored as a separate JSON file with $type discriminator.
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

    #region Secure Operations

    /// <summary>
    /// Gets a node by path, applying security filtering.
    /// Emits null if the user doesn't have read permission.
    /// Default implementation delegates to <see cref="GetNode"/> (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Observable emitting the node, or null if not found / not authorized</returns>
    IObservable<MeshNode?> GetNodeSecure(string path, string? userId, JsonSerializerOptions options)
        => GetNode(path, options);

    /// <summary>
    /// Gets child nodes, filtering out those the user cannot read.
    /// Default implementation delegates to <see cref="GetChildrenAsync"/> (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="parentPath">Parent path (empty or null for root level)</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Observable of accessible child nodes</returns>
    IObservable<MeshNode> GetChildrenSecure(string? parentPath, string? userId, JsonSerializerOptions options)
        => ObservableTopNExtensions.ToObservableSequence(GetChildrenAsync(parentPath, options));

    /// <summary>
    /// Gets descendant nodes, filtering out those the user cannot read.
    /// Default implementation delegates to <see cref="GetDescendantsAsync"/> (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="parentPath">Parent path</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Observable of accessible descendant nodes</returns>
    IObservable<MeshNode> GetDescendantsSecure(string? parentPath, string? userId, JsonSerializerOptions options)
        => ObservableTopNExtensions.ToObservableSequence(GetDescendantsAsync(parentPath, options));

    #endregion
}
