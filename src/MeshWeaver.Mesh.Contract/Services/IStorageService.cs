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
    /// Gets all child nodes at the specified parent path. Cold IObservable —
    /// Subscribe triggers the read; emits one snapshot collection (and may emit
    /// further snapshots if a backing implementation is live). Composes safely
    /// from hub-handler contexts; no Task await on the calling scheduler. Per
    /// Doc/Architecture/AsynchronousCalls.md.
    /// </summary>
    IObservable<IReadOnlyCollection<MeshNode>> GetChildren(string? parentPath, JsonSerializerOptions options) =>
        ObservableTopNExtensions.ToObservableSequence(GetChildrenAsync(parentPath, options))
            .ToList()
            .Select(list => (IReadOnlyCollection<MeshNode>)list);

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
    /// Creates or updates a node. Cold IObservable — Subscribe triggers the write.
    /// Composes via SelectMany; never awaits a Task internally between layers.
    /// The Task→IObservable bridge sits at the leaf <see cref="IStorageAdapter"/>
    /// boundary, scheduled on TaskPool, so the write never runs on a hub scheduler.
    /// </summary>
    IObservable<MeshNode> SaveNode(MeshNode node, JsonSerializerOptions options);

    /// <summary>
    /// Deletes a node and optionally its descendants. Cold IObservable —
    /// Subscribe triggers the delete.
    /// </summary>
    IObservable<string> DeleteNode(string path, bool recursive = false);

    /// <summary>
    /// Moves a node and all its descendants to a new path. Cold IObservable —
    /// Subscribe triggers the move. Comments associated with moved nodes are
    /// also migrated. Errors flow via OnError (e.g. source doesn't exist or
    /// target already exists).
    /// </summary>
    IObservable<MeshNode> MoveNode(string sourcePath, string targetPath, JsonSerializerOptions options);

    /// <summary>
    /// Searches nodes by query text within their Name or Content.
    /// </summary>
    /// <param name="parentPath">Parent path to search under (null for all)</param>
    /// <param name="query">Search query</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Async enumerable of matching nodes</returns>
    IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query, JsonSerializerOptions options);

    /// <summary>
    /// Checks if a node exists at the given path. Cold IObservable — Subscribe
    /// triggers the read.
    /// </summary>
    IObservable<bool> Exists(string path);

    /// <summary>
    /// Finds the node whose path is the longest prefix of the given full path.
    /// Uses a single query instead of iterating through ancestor paths. Cold
    /// IObservable — Subscribe triggers the read. Default impl emits (null, 0).
    /// </summary>
    IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => System.Reactive.Linq.Observable.Return<(MeshNode?, int)>((null, 0));

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
    /// Adds a comment to a node. Cold IObservable — Subscribe triggers the write.
    /// </summary>
    IObservable<Comment> AddComment(Comment comment, JsonSerializerOptions options);

    /// <summary>
    /// Deletes a comment by ID. Cold IObservable — Subscribe triggers the delete.
    /// </summary>
    IObservable<string> DeleteComment(string commentId);

    /// <summary>
    /// Gets a single comment by ID. Cold IObservable — Subscribe triggers the
    /// read. Emits null if not found.
    /// </summary>
    IObservable<Comment?> GetComment(string commentId);

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
    /// Saves objects to a node's partition folder. Cold IObservable — Subscribe
    /// triggers the write. Each object is stored as a separate record with $type
    /// discriminator.
    /// </summary>
    IObservable<IReadOnlyCollection<object>> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options);

    /// <summary>
    /// Deletes all objects from a node's partition folder (or sub-path). Cold
    /// IObservable — Subscribe triggers the delete.
    /// </summary>
    IObservable<string> DeletePartitionObjects(string nodePath, string? subPath = null);

    /// <summary>
    /// Gets the newest modification timestamp across all objects in a partition
    /// (or sub-path). Used for cache invalidation. Cold IObservable — Subscribe
    /// triggers the read.
    /// </summary>
    IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null);

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
